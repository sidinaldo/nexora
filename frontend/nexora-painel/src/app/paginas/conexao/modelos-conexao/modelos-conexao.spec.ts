import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { ConexaoServico } from '../../../nucleo/servicos/conexao.servico';
import { ToastServico } from '../../../nucleo/toast/toast.servico';
import { ModeloMensagem, NovoModelo } from '../../../nucleo/modelos';
import { ModelosConexao } from './modelos-conexao';

/** OS TEMPLATES DE UM NÚMERO DA API OFICIAL (INT-XX).
 *
 *  O que este arquivo segura: os botões seguem o STATUS que o servidor devolveu (só o rascunho se
 *  edita e se envia; em revisão só se atualiza), o motivo da recusa aparece, e o erro do servidor
 *  chega ao formulário em vez de sumir. */
describe('ModelosConexao', () => {
  function modelo(over: Partial<ModeloMensagem> = {}): ModeloMensagem {
    return {
      id: 1, conexaoId: 2, nome: 'retomada', categoria: 'utility', idioma: 'pt_BR',
      corpo: 'Olá {{nome}}, podemos continuar?', variaveis: ['nome'], status: 'rascunho',
      motivoRejeicao: null, atualizadoEm: '2026-10-09T12:00:00Z', ...over
    };
  }

  class ServicoFalso {
    lista: ModeloMensagem[] = [];
    criados: { conexaoId: number; novo: NovoModelo }[] = [];
    submetidos: number[] = [];
    erroAoCriar: string | null = null;
    respostaDoEnvio: ModeloMensagem | null = null;

    listarModelos(): Observable<ModeloMensagem[]> { return of(this.lista); }
    criarModelo(conexaoId: number, novo: NovoModelo): Observable<{ id: number }> {
      this.criados.push({ conexaoId, novo });
      if (this.erroAoCriar !== null) return throwError(() => ({ error: { erro: this.erroAoCriar } }));
      return of({ id: 9 });
    }
    editarModelo(): Observable<void> { return of(undefined); }
    excluirModelo(): Observable<void> { return of(undefined); }
    submeterModelo(id: number): Observable<ModeloMensagem> {
      this.submetidos.push(id);
      return of(this.respostaDoEnvio ?? modelo({ id, status: 'enviado' }));
    }
    atualizarModelo(id: number): Observable<ModeloMensagem> { return of(modelo({ id, status: 'aprovado' })); }
  }

  class ToastFalso {
    sucessos: string[] = [];
    erros: string[] = [];
    sucesso(m: string) { this.sucessos.push(m); }
    erro(m: string) { this.erros.push(m); }
    info() { }
  }

  let servico: ServicoFalso;
  let toast: ToastFalso;
  let fixture: ComponentFixture<ModelosConexao>;
  let c: ModelosConexao;

  beforeEach(() => {
    servico = new ServicoFalso();
    toast = new ToastFalso();
    TestBed.configureTestingModule({
      imports: [ModelosConexao],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ConexaoServico, useValue: servico },
        { provide: ToastServico, useValue: toast }
      ]
    });
  });

  async function montar() {
    fixture = TestBed.createComponent(ModelosConexao);
    c = fixture.componentInstance;
    fixture.componentRef.setInput('conexaoId', 2);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function texto(): string { return (fixture.nativeElement as HTMLElement).textContent ?? ''; }

  function rotulos(): string[] {
    return [...(fixture.nativeElement as HTMLElement).querySelectorAll('li button')]
      .map(b => b.textContent?.trim() ?? '');
  }

  it('o rascunho se envia, se edita e se apaga', async () => {
    servico.lista = [modelo()];
    await montar();

    expect(texto()).toContain('Rascunho');
    expect(rotulos()).toEqual(['Enviar para revisão', 'Editar', 'Apagar']);
  });

  it('em revisão, só dá para atualizar o status', async () => {
    servico.lista = [modelo({ status: 'enviado' })];
    await montar();

    expect(texto()).toContain('Em revisão na Meta');
    expect(rotulos()).toEqual(['Atualizar status']);
  });

  it('aprovado não tem o que mexer aqui', async () => {
    servico.lista = [modelo({ status: 'aprovado' })];
    await montar();

    expect(texto()).toContain('Aprovado');
    expect(rotulos()).toEqual([]);
  });

  it('recusado mostra o motivo da Meta e pode ser apagado', async () => {
    servico.lista = [modelo({ status: 'rejeitado', motivoRejeicao: 'Formato inválido: confira as variáveis.' })];
    await montar();

    expect(texto()).toContain('Recusado');
    expect(texto()).toContain('Formato inválido: confira as variáveis.');
    expect(rotulos()).toEqual(['Apagar']);
  });

  it('cria o rascunho com o que foi digitado e recarrega a lista', async () => {
    await montar();
    c.novo();
    c.fNome.set('Retomada');
    c.fCategoria.set('marketing');
    c.fCorpo.set('Olá {{nome}}, temos novidade!');

    servico.lista = [modelo({ id: 9, nome: 'retomada' })];
    c.salvar();
    fixture.detectChanges();

    expect(servico.criados).toEqual([{
      conexaoId: 2, novo: { nome: 'Retomada', categoria: 'marketing', corpo: 'Olá {{nome}}, temos novidade!' }
    }]);
    expect(c.formularioAberto()).toBeFalse();
    expect(c.lista().map(m => m.id)).toEqual([9]);
  });

  /** A regra (variável no começo, nome repetido) é do servidor: o motivo dele vai para o formulário. */
  it('o erro do servidor fica no formulário, que continua aberto', async () => {
    servico.erroAoCriar = 'A Meta não aceita template que começa ou termina com variável.';
    await montar();
    c.novo();
    c.fNome.set('x');
    c.fCorpo.set('{{nome}} oi');

    c.salvar();
    fixture.detectChanges();

    expect(c.formularioAberto()).toBeTrue();
    expect(texto()).toContain('começa ou termina com variável');
  });

  it('enviar para revisão troca o item pelo que o servidor devolveu', async () => {
    servico.lista = [modelo()];
    await montar();

    c.enviarParaRevisao(c.lista()[0]);
    fixture.detectChanges();

    expect(servico.submetidos).toEqual([1]);
    expect(c.lista()[0].status).toBe('enviado');
    expect(rotulos()).toEqual(['Atualizar status']);
  });

  it('a variável entra onde está o cursor', async () => {
    await montar();
    c.novo();
    fixture.detectChanges();
    c.fCorpo.set('Olá , tudo bem?');
    const campo = (fixture.nativeElement as HTMLElement).querySelector('textarea') as HTMLTextAreaElement;
    campo.value = 'Olá , tudo bem?';
    campo.setSelectionRange(4, 4);

    c.inserir('nome', campo);

    expect(c.fCorpo()).toBe('Olá {{nome}}, tudo bem?');
  });
});
