import { setupZoneTestEnv } from 'jest-preset-angular/setup-env/zone';

setupZoneTestEnv();

import { TestBed, ComponentFixture } from '@angular/core/testing';
import { DataTableComponent, ColumnDef } from './data-table.component';

describe('DataTableComponent', () => {
  let component: DataTableComponent;
  let fixture: ComponentFixture<DataTableComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DataTableComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(DataTableComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('columns', []);
    fixture.componentRef.setInput('data', []);
    fixture.detectChanges();
  });

  describe('formatValue', () => {
    it('should round numbers to 2 decimals', () => {
      expect(component.formatValue(3.14159, 'number')).toBe('3.14');
    });

    it('should format currency values', () => {
      expect(component.formatValue(100.5, 'currency')).toBe('100.50');
    });

    it('should return - for null/undefined', () => {
      expect(component.formatValue(null)).toBe('-');
      expect(component.formatValue(undefined)).toBe('-');
    });
  });

  describe('sort', () => {
    it('should sort rows in ascending order when a numeric column header is clicked', () => {
      const columns: ColumnDef[] = [
        { key: 'name', label: 'Name' },
        { key: 'value', label: 'Value', format: 'number' },
      ];
      const data = [
        { name: 'B', value: 2 },
        { name: 'A', value: 1 },
        { name: 'C', value: 3 },
      ];

      fixture.componentRef.setInput('columns', columns);
      fixture.componentRef.setInput('data', data);
      fixture.detectChanges();

      // Click Value header — first click = ascending
      const headers = fixture.nativeElement.querySelectorAll('th');
      expect(headers.length).toBe(2);
      headers[1].click();
      fixture.detectChanges();

      const cells = fixture.nativeElement.querySelectorAll('td');
      const firstCellText = cells[0]?.textContent?.trim();
      expect(firstCellText).toBe('A');
    });

    it('should toggle sort direction on repeated clicks', () => {
      const columns: ColumnDef[] = [
        { key: 'name', label: 'Name' },
        { key: 'value', label: 'Value', format: 'number' },
      ];
      const data = [
        { name: 'B', value: 2 },
        { name: 'A', value: 1 },
        { name: 'C', value: 3 },
      ];

      fixture.componentRef.setInput('columns', columns);
      fixture.componentRef.setInput('data', data);
      fixture.detectChanges();

      const headers = fixture.nativeElement.querySelectorAll('th');
      // 1st click: ascending
      headers[1].click();
      fixture.detectChanges();
      let cells = fixture.nativeElement.querySelectorAll('td');
      expect(cells[0]?.textContent?.trim()).toBe('A');

      // 2nd click: descending
      headers[1].click();
      fixture.detectChanges();
      cells = fixture.nativeElement.querySelectorAll('td');
      expect(cells[0]?.textContent?.trim()).toBe('C');

      // 3rd click: reset sort
      headers[1].click();
      fixture.detectChanges();
      cells = fixture.nativeElement.querySelectorAll('td');
      expect(cells[0]?.textContent?.trim()).toBe('B');
    });

    it('should filter rows by search term', () => {
      const columns: ColumnDef[] = [
        { key: 'name', label: 'Name' },
        { key: 'value', label: 'Value', format: 'number' },
      ];
      const data = [
        { name: 'Apple', value: 1 },
        { name: 'Banana', value: 2 },
        { name: 'Cherry', value: 3 },
      ];

      fixture.componentRef.setInput('columns', columns);
      fixture.componentRef.setInput('data', data);
      fixture.detectChanges();

      // Type in search
      const input = fixture.nativeElement.querySelector('input');
      expect(input).toBeTruthy();
      input.value = 'app';
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      // Wait for debounce
      jest.advanceTimersByTime?.(200);
      fixture.detectChanges();

      const cells = fixture.nativeElement.querySelectorAll('td');
      expect(cells[0]?.textContent?.trim()).toBe('Apple');
    });
  });
});
