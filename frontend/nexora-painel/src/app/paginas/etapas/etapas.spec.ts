import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { EtapaConfigDto } from '../../nucleo/modelos';
import { Etapas } from './etapas';

/** A tela de etapas guarda decisões que o servidor também guarda — de propósito.
 *
 *  O servidor é quem decide; a tela existe para o dono não descobrir a regra levando um 400
 *  depois de clicar. Estes testes cobrem o que só a tela faz: qual botão fica disponível, qual
 *  ordem é enviada, e o que acontece quando a API recusa. */
describe('etapas do funil', () => {
  const FUNIL: EtapaConfigDto[] = [
    { id: 1, nome: 'Novo Lead', ordem: 1, cor: '#7FA88B', eGanho: false, contatos: 12 },
    { id: 2, nome: 'Proposta', ordem: 2, cor: '#3E7554', eGanho: false, contatos: 4 },
    { id: 3, nome: 'Venda', ordem: 3, cor: '#1E4028', eGanho: true, contatos: 7 }
  ];

  let componente: Etapas;
  let http: HttpTestingController;

  function montar(funil: EtapaConfigDto[] = FUNIL) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    const fixture = TestBed.createComponent(Etapas);
    componente = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);

    fixture.detectChanges();
    http.expectOne(r => r.url.includes('/etapas')).flush(funil);
    return fixture;
  }

  afterEach(() => TestBed.resetTestingModule());

  it('a etapa de ganho não oferece o botão de apagar', () => {
    montar();
    const ganho = FUNIL.find(e => e.eGanho)!;
    expect(componente.podeApagar(ganho)).toBeFalse();
  });

  it('A ÚLTIMA ETAPA NÃO-GANHO NÃO PODE SER APAGADA', () => {
    // ===================== A INVARIANTE QUE O BANCO NÃO GARANTE =====================
    // O lead novo entra na etapa de MENOR ordem. Sobrando só a de ganho, todo contato criado já
    // nasceria ganho — e a "porta única do ganho" cairia por dentro, sem erro nenhum.
    // ===============================================================================
    montar([
      { id: 1, nome: 'Novo Lead', ordem: 1, cor: '#7FA88B', eGanho: false, contatos: 0 },
      { id: 3, nome: 'Venda', ordem: 2, cor: '#1E4028', eGanho: true, contatos: 7 }
    ]);

    expect(componente.podeApagar(componente.lista()[0]))
      .withContext('é a única etapa além da de ganho').toBeFalse();
  });

  it('com duas não-ganho, as duas podem ser apagadas', () => {
    montar();
    expect(componente.podeApagar(FUNIL[0])).toBeTrue();
    expect(componente.podeApagar(FUNIL[1])).toBeTrue();
  });

  it('MOVER MANDA A ORDEM INTEIRA, não um "sobe uma posição"', () => {
    // É o que torna a operação idempotente: duplo clique ou retry de rede repetem a mesma
    // requisição e chegam ao mesmo lugar.
    montar();

    componente.mover(0, 1);   // Novo Lead desce

    const req = http.expectOne(r => r.url.endsWith('/etapas/ordem'));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body.ids).toEqual([2, 1, 3]);

    req.flush(null);
    http.expectOne(r => r.url.includes('/etapas')).flush(FUNIL);   // o carregar() de volta
  });

  it('mover para fora da lista não faz nada', () => {
    montar();

    componente.mover(0, -1);                       // já é a primeira
    componente.mover(FUNIL.length - 1, 1);         // já é a última

    http.expectNone(r => r.url.endsWith('/etapas/ordem'));
  });

  it('SE A API RECUSA A REORDENAÇÃO, A TELA VOLTA À VERDADE DO SERVIDOR', () => {
    // A tela pinta a nova ordem na hora, antes da resposta. Sem recarregar no erro, ela ficaria
    // mostrando uma ordem que o banco não tem — e o dono sairia da tela acreditando nela.
    montar();

    componente.mover(0, 1);
    expect(componente.lista().map(e => e.id))
      .withContext('pintou otimista').toEqual([2, 1, 3]);

    http.expectOne(r => r.url.endsWith('/etapas/ordem'))
      .flush({ erro: 'não deu' }, { status: 400, statusText: 'Bad Request' });

    // Recarregou, e a verdade do servidor venceu.
    http.expectOne(r => r.url.includes('/etapas')).flush(FUNIL);
    expect(componente.lista().map(e => e.id)).toEqual([1, 2, 3]);
  });

  it('apagar etapa COM contatos manda destino; SEM contatos não manda', () => {
    montar();

    // Com contatos: o destino pré-selecionado é a etapa anterior — para onde o contato "volta"
    // naturalmente quando a coluna some.
    componente.pedirRemocao(FUNIL[1]);
    expect(componente.destino()).toBe(1);

    componente.confirmarRemocao();
    let req = http.expectOne(r => r.url.includes('/etapas/2'));
    expect(req.request.method).toBe('DELETE');
    expect(req.request.url).toContain('destino=1');
    req.flush(null);
    http.expectOne(r => r.url.includes('/etapas')).flush(FUNIL);

    // Sem contatos: nada de destino na URL.
    const vazia: EtapaConfigDto = { ...FUNIL[1], id: 9, nome: 'Vazia', contatos: 0 };
    componente.lista.set([FUNIL[0], vazia, FUNIL[2]]);
    componente.pedirRemocao(vazia);
    componente.confirmarRemocao();

    req = http.expectOne(r => r.url.includes('/etapas/9'));
    expect(req.request.url).not.toContain('destino');
  });

  it('os destinos possíveis nunca incluem a etapa que está sendo apagada', () => {
    montar();
    componente.pedirRemocao(FUNIL[1]);
    expect(componente.destinos().map(d => d.id)).toEqual([1, 3]);
  });

  it('o teto de etapas esconde o formulário antes de o dono digitar', () => {
    // Cortesia, não segurança: o servidor recusa de qualquer jeito. Mas deixar alguém escolher
    // nome e cor para levar 400 no fim é desperdiçar o trabalho dele.
    montar();
    expect(componente.cheio()).toBeFalse();

    componente.lista.set(Array.from({ length: componente.maximo }, (_, i) => ({
      ...FUNIL[0], id: i + 1, nome: `Etapa ${i}`
    })));
    expect(componente.cheio()).toBeTrue();
  });

  it('a tela diz qual etapa é a porta de entrada', () => {
    // É a consequência menos óbvia de reordenar: mover uma coluna para o topo muda onde TODO
    // lead futuro nasce — o do WhatsApp e o do formulário do site.
    montar();
    expect(componente.primeira()?.nome).toBe('Novo Lead');

    componente.lista.set([FUNIL[1], FUNIL[0], FUNIL[2]]);
    expect(componente.primeira()?.nome).toBe('Proposta');
  });
});
