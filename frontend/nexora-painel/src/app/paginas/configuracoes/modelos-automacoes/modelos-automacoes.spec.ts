import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';
import { ConexaoServico } from '../../../nucleo/servicos/conexao.servico';
import { ToastServico } from '../../../nucleo/toast/toast.servico';
import { EscolhaDasAutomacoes, ModelosDasAutomacoes } from '../../../nucleo/modelos';
import { ModelosAutomacoes } from './modelos-automacoes';

/** O TEMPLATE DE CADA AUTOMAÇÃO (INT-XX).
 *
 *  O que este arquivo segura: empresa só com QR code não vê a seção; a escolha que veio do
 *  servidor aparece selecionada e volta inteira no salvar; e a escolha que perdeu a aprovação da
 *  Meta é avisada, em vez de parecer que está tudo certo. */
describe('ModelosAutomacoes', () => {
  class ServicoFalso {
    resposta: ModelosDasAutomacoes = { followUp: null, lembrete: null, nps: null, aprovados: [] };
    salvos: EscolhaDasAutomacoes[] = [];
    modelosDasAutomacoes(): Observable<ModelosDasAutomacoes> { return of(this.resposta); }
    definirModelosDasAutomacoes(e: EscolhaDasAutomacoes): Observable<void> {
      this.salvos.push(e);
      return of(undefined);
    }
  }

  const toast = { sucesso: () => { }, erro: () => { }, info: () => { } };
  let servico: ServicoFalso;
  let fixture: ComponentFixture<ModelosAutomacoes>;
  let c: ModelosAutomacoes;

  beforeEach(() => {
    servico = new ServicoFalso();
    TestBed.configureTestingModule({
      imports: [ModelosAutomacoes],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ConexaoServico, useValue: servico },
        { provide: ToastServico, useValue: toast }
      ]
    });
  });

  function montar() {
    fixture = TestBed.createComponent(ModelosAutomacoes);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  function texto(): string { return (fixture.nativeElement as HTMLElement).textContent ?? ''; }

  it('sem template aprovado nem escolha, a seção não aparece', () => {
    montar();
    expect(texto().trim()).toBe('');
  });

  it('a escolha do servidor aparece e volta inteira no salvar', () => {
    servico.resposta = {
      followUp: 3, lembrete: null, nps: 4,
      aprovados: [
        { id: 3, nome: 'retomada', conexao: 'Oficial', corpo: 'Oi {{nome}}!' },
        { id: 4, nome: 'pesquisa', conexao: 'Oficial', corpo: 'De 0 a 10?' }
      ]
    };
    montar();

    expect(texto()).toContain('Templates das automações');
    expect(texto()).toContain('retomada (Oficial)');
    expect(c.fFollowUp()).toBe(3);

    c.fLembrete.set(3);
    c.salvar();

    expect(servico.salvos).toEqual([{ followUp: 3, lembrete: 3, nps: 4 }]);
  });

  /** A Meta pausou o template depois da escolha: a automação não sai até trocar, e a tela diz. */
  it('a escolha que perdeu a aprovação é avisada', () => {
    servico.resposta = { followUp: 9, lembrete: null, nps: null, aprovados: [] };
    montar();

    expect(texto()).toContain('não está mais aprovado pela Meta');
  });
});
